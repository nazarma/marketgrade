# Changelog

All notable changes to Marketgrade are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-10-30

The first public release, for macOS (Apple Silicon and Intel), Windows and Linux. The installers are not
code signed; the README explains how to open them.

### Added

- Live connections to the public BTC/USD(T) market data of Kraken, Coinbase and Binance, with automatic
  reconnects.
- A local order book per exchange, checked after every message with the exchange's own method (Kraken's
  checksum, Coinbase's sequence numbers, Binance's update ID chain) and repaired by itself when a check fails.
- Delay measurement against NTP time, processing time per message, gap and reconnect tracking, and per-minute
  history kept on this computer.
- Recording of every message to compressed hourly files, with retention and low-disk protection.
- Four screens: Overview, Live flow (an animation of book changes and trades as they arrive), Data health
  (connection timeline, delay and volume, gap log) and Settings (recording, history, About).
- Installers: DMG for macOS (Apple Silicon and Intel), NSIS installer for Windows, AppImage and `.deb` for
  Linux.

[0.1.0]: https://github.com/nazarma/marketgrade/releases/tag/v0.1.0
