using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Marketgrade.Engine.Recording;

public enum RecordingState
{
    Recording,
    Off,
    PausedLowDisk,
    Error,
}

/// <summary>
/// Writes every record of one exchange to hourly files. Records are collected in a write buffer, which
/// goes to the file when it is full or when the write interval has passed. The recorder is the
/// exchange's second consumer and never slows the connection: the connection drops what does not fit
/// in the recorder's queue and stamps the dropped range onto the next record, which becomes a
/// <see cref="RecordType.Gap"/> record here. Allocation-free per record.
/// </summary>
internal sealed class Recorder
{
    /// <summary>The lock is released after this many records, so the interval timer and new settings get their turn.</summary>
    private const int MaxBatch = 256;

    private readonly ExchangeAdapter _adapter;
    private readonly RecordingManager _manager;
    private readonly Lock _lock = new();
    private readonly TagList _exchangeTag;
    private readonly PeriodicTimer _timer;

    private RecordingSettings _settings;
    private byte[] _buffer;
    private int _buffered;

    /// <summary>Messages in the write buffer; they move to <see cref="_skipped"/> if writing the buffer fails.</summary>
    private SeqRange _unflushed;

    /// <summary>Messages not recorded since the last record written; written as a gap before the next one.</summary>
    private SeqRange _skipped;

    private bool _writing;
    private bool _failed;
    private long _failedAt;
    private string? _recoverBeforeAppend;

    private SafeFileHandle? _file;
    private string? _filePath;
    private long _fileHour;
    private long _fileOffset;

    private int _state;
    private string? _lastError;

    public Recorder(ExchangeAdapter adapter, RecordingManager manager)
    {
        _adapter = adapter;
        _manager = manager;
        _exchangeTag = new TagList { { "exchange", adapter.Name } };
        _settings = manager.Settings;
        _buffer = new byte[_settings.WriteBufferBytes];
        _timer = new PeriodicTimer(_settings.WriteInterval, manager.Time);
        _state = (int)(_settings.Enabled ? RecordingState.Recording : RecordingState.Off);
    }

    public ExchangeAdapter Adapter => _adapter;

    public RecordingState State => (RecordingState)Volatile.Read(ref _state);

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>The file being written, which retention and compression must leave alone.</summary>
    public string? ActiveFile => Volatile.Read(ref _filePath);

    /// <summary>Runs until the queue is completed and empty, so every buffer is released, then closes the file.</summary>
    public async Task RunAsync(ChannelReader<MessageBuffer> reader)
    {
        lock (_lock)
        {
            Recover();
        }
        var flusher = FlushOnIntervalAsync();
        try
        {
            // No cancellation token: the channel then reuses its waiter and the loop does not allocate.
            while (await reader.WaitToReadAsync())
            {
                lock (_lock)
                {
                    BeginBatch();
                    for (var i = 0; i < MaxBatch && reader.TryRead(out var buffer); i++)
                    {
                        Process(buffer);
                    }
                }
            }
        }
        finally
        {
            _timer.Dispose();
            await flusher;
            lock (_lock)
            {
                CloseFile();
            }
        }
    }

    /// <summary>Applies new settings and decides whether this batch is written. Called with the lock held.</summary>
    internal void BeginBatch()
    {
        try
        {
            ApplySettings();
            var writing = _settings.Enabled
                && !_manager.Paused
                && (!_failed || _manager.Time.GetElapsedTime(_failedAt) >= _manager.Options.RecordingRetryDelay);
            if (_writing && !writing)
            {
                Close();
            }
            _writing = writing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(e);
        }
        SetState(!_settings.Enabled ? RecordingState.Off
            : _manager.Paused ? RecordingState.PausedLowDisk
            : _failed ? RecordingState.Error
            : RecordingState.Recording);
    }

    /// <summary>Writes one record, or counts it as skipped, and releases the buffer. Called with the lock held.</summary>
    internal void Process(MessageBuffer buffer)
    {
        try
        {
            if (_writing)
            {
                Write(buffer);
            }
            else
            {
                Skip(buffer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(e);
        }
        finally
        {
            buffer.Release();
        }
    }

    private void Skip(MessageBuffer buffer)
    {
        _skipped.Add(buffer.GapFirstSeq, buffer.GapLastSeq, buffer.GapCount);
        if (buffer.Type == RecordType.Message)
        {
            _skipped.Add(buffer.Seq);
        }
    }

    private async Task FlushOnIntervalAsync()
    {
        // Ends when the timer is disposed. Flushing here covers quiet periods with no new records.
        while (await _timer.WaitForNextTickAsync())
        {
            lock (_lock)
            {
                BeginBatch();
                if (_buffered > 0 && _file is not null)
                {
                    try
                    {
                        Flush();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Fail(e);
                    }
                }
            }
        }
    }

    private void ApplySettings()
    {
        var settings = _manager.Settings;
        if (ReferenceEquals(settings, _settings))
        {
            return;
        }
        if (settings.Folder != _settings.Folder)
        {
            Close();
        }
        if (settings.WriteBufferBytes != _buffer.Length)
        {
            if (_file is not null)
            {
                Flush();
            }
            _buffer = new byte[settings.WriteBufferBytes];
        }
        if (settings.WriteInterval != _settings.WriteInterval)
        {
            _timer.Period = settings.WriteInterval;
        }
        var folderChanged = settings.Folder != _settings.Folder;
        _settings = settings;
        if (folderChanged)
        {
            Recover();
        }
    }

    /// <summary>Repairs this exchange's files in the current folder and queues finished hours for compression.</summary>
    private void Recover()
    {
        var exchangeFolder = RecordingFiles.ExchangeFolder(_settings.Folder, _adapter.Name);
        try
        {
            var cut = RecordingRecovery.RecoverExchange(exchangeFolder, _manager.Logger);
            if (cut > 0)
            {
                _manager.Events.Add(_adapter.Name, "Recording repaired", $"{cut} bytes cut after the last valid record");
            }
            var currentHour = RecordingFiles.HourOf(_manager.Time.GetUtcNow());
            foreach (var file in RecordingFiles.EnumerateExchange(exchangeFolder))
            {
                if (!file.Compressed && file.Hour < currentHour)
                {
                    _manager.QueueCompression(file.Path);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _manager.Logger.LogWarning("{Exchange}: could not check recordings in {Folder}: {Error}", _adapter.Name, exchangeFolder, e.Message);
        }
    }

    private void Write(MessageBuffer buffer)
    {
        var hour = buffer.ArrivalUnixNs / RecordingFiles.NsPerHour;
        try
        {
            if (_file is null)
            {
                Open(hour);
            }
            else if (hour > _fileHour)
            {
                // Forward only: if the clock moved back, records stay in the current file.
                Close();
                Open(hour);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Skip(buffer);
            throw;
        }

        // Ranges first, so a failed write moves everything in the buffer back to the skipped range.
        var gap = _skipped;
        gap.Add(buffer.GapFirstSeq, buffer.GapLastSeq, buffer.GapCount);
        _skipped.Clear();
        _unflushed.Add(gap);
        if (buffer.Type == RecordType.Message)
        {
            _unflushed.Add(buffer.Seq);
        }

        if (!gap.IsEmpty)
        {
            Span<byte> payload = stackalloc byte[RecordFormat.GapPayloadSize];
            RecordFormat.WriteGapPayload(payload, gap.First, gap.Last, gap.Count);
            WriteRecord(RecordType.Gap, payload, gap.First - 1, buffer.ArrivalUnixNs, buffer.ConnectionId);
        }
        WriteRecord(buffer.Type, buffer.Span, buffer.Seq, buffer.ArrivalUnixNs, buffer.ConnectionId);
    }

    private void WriteRecord(RecordType type, ReadOnlySpan<byte> payload, long seq, long arrivalUnixNs, long connectionId)
    {
        var size = RecordFormat.RecordHeaderSize + payload.Length;
        if (_buffer.Length - _buffered < size)
        {
            Flush();
        }
        if (size <= _buffer.Length)
        {
            var destination = _buffer.AsSpan(_buffered, size);
            RecordFormat.WriteRecordHeader(destination, type, payload, seq, arrivalUnixNs, connectionId);
            payload.CopyTo(destination[RecordFormat.RecordHeaderSize..]);
            _buffered += size;
        }
        else
        {
            // Larger than the whole write buffer (which Flush just emptied): straight to the file.
            RecordFormat.WriteRecordHeader(_buffer, type, payload, seq, arrivalUnixNs, connectionId);
            WriteToFile(_buffer.AsSpan(0, RecordFormat.RecordHeaderSize));
            WriteToFile(payload);
            _unflushed.Clear();
        }
    }

    private void Flush()
    {
        if (_buffered > 0)
        {
            WriteToFile(_buffer.AsSpan(0, _buffered));
            _buffered = 0;
            if (_failed && _writing)
            {
                _failed = false;
                SetState(RecordingState.Recording);
            }
        }
        _unflushed.Clear();
    }

    private void WriteToFile(ReadOnlySpan<byte> bytes)
    {
        RandomAccess.Write(_file!, bytes, _fileOffset);
        _fileOffset += bytes.Length;
        _manager.Metrics?.RecordingBytesWritten.Add(bytes.Length, _exchangeTag);
    }

    /// <summary>
    /// Opens the file for <paramref name="hour"/> and appends to it, or creates it. An hour that is
    /// already compressed, or a file that is not ours, continues in the next part (<c>HH-2.mgrec</c>).
    /// </summary>
    private void Open(long hour)
    {
        Debug.Assert(_buffered == 0, "The write buffer belongs to the previous file");
        var folder = _settings.Folder;
        for (var part = 1; ; part++)
        {
            if (File.Exists(RecordingFiles.PathFor(folder, _adapter.Name, hour, part, compressed: true)))
            {
                continue;
            }
            var path = RecordingFiles.PathFor(folder, _adapter.Name, hour, part);
            if (path == _recoverBeforeAppend)
            {
                _recoverBeforeAppend = null;
                RecordingRecovery.RecoverFile(path, _manager.Logger);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
            var length = RandomAccess.GetLength(file);
            if (length > 0 && !IsOwnFile(file))
            {
                file.Dispose();
                continue;
            }

            _file = file;
            _fileHour = hour;
            _fileOffset = length;
            Volatile.Write(ref _filePath, path);
            if (length == 0)
            {
                var created = (_manager.Time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
                _buffered = RecordFormat.WriteFileHeader(_buffer, _adapter.Id, _adapter.Name, created);
            }
            return;
        }
    }

    private bool IsOwnFile(SafeFileHandle file)
    {
        Span<byte> start = stackalloc byte[11 + 255 + 8];
        var read = RandomAccess.Read(file, start, 0);
        return RecordFormat.TryReadFileHeader(start[..read], out var header) > 0 && header.ExchangeId == _adapter.Id;
    }

    /// <summary>Flushes and closes the current file; a finished hour goes to compression.</summary>
    private void Close()
    {
        if (_file is null)
        {
            return;
        }
        Flush();
        RandomAccess.FlushToDisk(_file);
        _file.Dispose();
        _file = null;
        var path = _filePath!;
        Volatile.Write(ref _filePath, null);
        if (_fileHour < RecordingFiles.HourOf(_manager.Time.GetUtcNow()))
        {
            _manager.QueueCompression(path);
        }
    }

    /// <summary>Closes the file after the last record. Called with the lock held.</summary>
    internal void CloseFile()
    {
        try
        {
            Close();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(e);
        }
    }

    /// <summary>Gives up the current file and batch after a write error; writing is retried after a delay.</summary>
    private void Fail(Exception e)
    {
        _skipped.Add(_unflushed);
        _unflushed.Clear();
        _buffered = 0;
        if (_file is not null)
        {
            // The file may end in a partial record; it is repaired before it is appended to again.
            _recoverBeforeAppend = _filePath;
            _file.Dispose();
            _file = null;
            Volatile.Write(ref _filePath, null);
        }
        // Retries that fail the same way are not logged again.
        if (!_failed || e.Message != _lastError)
        {
            _manager.Events.Add(_adapter.Name, "Recording error", e.Message);
            _manager.Logger.LogWarning("{Exchange}: recording failed, retrying every {Delay}: {Error}",
                _adapter.Name, _manager.Options.RecordingRetryDelay, e.Message);
        }
        _writing = false;
        _failed = true;
        _failedAt = _manager.Time.GetTimestamp();
        Volatile.Write(ref _lastError, e.Message);
        SetState(RecordingState.Error);
    }

    private void SetState(RecordingState state) => Volatile.Write(ref _state, (int)state);
}
