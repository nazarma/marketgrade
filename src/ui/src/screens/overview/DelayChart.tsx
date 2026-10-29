import { useEffect, useRef, useState } from "react";
import uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import { exchangeInfo } from "@/lib/exchanges";
import { formatDelay, lowerBoundNote } from "@/lib/delay";
import { formatClock, formatMs } from "@/lib/format";
import { niceMax, type DelayChartData, type GapMarker } from "@/screens/overview/delayChartData";

const HEIGHT = 210;
const FONT = "11px 'Geist Mono Variable', ui-monospace, monospace";
const LABEL_FONT = "11px 'Geist Variable', system-ui, sans-serif";
const MUTED = "#8B95A5";
const GRID = "#1E2430";
const WARNING = "#F5B841";

/** Median delay per minute and exchange, one line each (colour and dash pattern), with a marker per gap. */
export function DelayChart({ data, markers }: { data: DelayChartData; markers: GapMarker[] }) {
  const container = useRef<HTMLDivElement>(null);
  const chart = useRef<uPlot | null>(null);
  const markersRef = useRef(markers);
  const [hover, setHover] = useState<number | null>(null);
  const exchangeKey = data.exchanges.map((e) => e.id).join(",");

  useEffect(() => {
    markersRef.current = markers;
    chart.current?.redraw(false);
  }, [markers]);

  useEffect(() => {
    const element = container.current;
    if (!element) {
      return;
    }
    const plot = new uPlot(
      {
        width: element.clientWidth,
        height: HEIGHT,
        padding: [22, 22, 0, 0],
        legend: { show: false },
        cursor: { drag: { x: false, y: false }, points: { size: 7 } },
        scales: {
          x: { time: true },
          y: { range: (_u, _min, max) => [0, niceMax(max)] },
        },
        axes: [
          {
            stroke: MUTED,
            font: FONT,
            size: 30,
            grid: { show: false },
            ticks: { show: false },
            values: (_u, splits) => splits.map((s) => formatClock(s * 1000)),
          },
          {
            stroke: MUTED,
            font: FONT,
            size: 48,
            grid: { stroke: GRID, width: 1 },
            ticks: { show: false },
            values: (_u, splits) => splits.map((v) => v.toLocaleString("en-US")),
          },
        ],
        series: [
          {},
          ...data.exchanges.map((e) => {
            const info = exchangeInfo(e.id);
            return {
              label: e.name,
              stroke: info.color,
              width: 2,
              dash: info.dash,
              spanGaps: false,
              points: { show: false },
            };
          }),
        ],
        hooks: {
          draw: [(u) => drawMarkers(u, markersRef.current)],
          setCursor: [(u) => setHover(u.cursor.idx ?? null)],
        },
      },
      [data.x, ...data.y],
      element,
    );
    chart.current = plot;
    const resize = new ResizeObserver(() =>
      plot.setSize({ width: element.clientWidth, height: HEIGHT }),
    );
    resize.observe(element);
    return () => {
      resize.disconnect();
      plot.destroy();
      chart.current = null;
    };
    // The chart is rebuilt only when the exchanges change; new data goes through setData.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [exchangeKey]);

  useEffect(() => {
    chart.current?.setData([data.x, ...data.y]);
  }, [data]);

  const hoverTime = hover === null ? undefined : data.x[hover];
  // The hovered minute's lower bounds, explained with the largest lead among them.
  const bounds =
    hover === null
      ? []
      : data.exchanges.flatMap((_e, i) =>
          data.lowerBound[i]?.[hover] && data.y[i]?.[hover] != null
            ? [data.aheadMs[i]?.[hover] ?? null]
            : [],
        );
  return (
    <div className="flex flex-col gap-1">
      <div className="lbl mono min-h-4" aria-live="off">
        {hoverTime !== undefined && hover !== null
          ? `${formatClock(hoverTime * 1000)}  ${data.exchanges.map((e, i) => `${e.name} ${formatDelay(data.y[i]?.[hover], data.lowerBound[i]?.[hover] ?? false)}`).join("  ·  ")}`
          : ""}
      </div>
      {/* The line is kept while any point is a lower bound, so the chart does not move under the pointer. */}
      {data.lowerBound.some((points) => points.includes(true)) && (
        <div className="lbl -mt-1 min-h-4">
          {bounds.length > 0 && lowerBoundNote(Math.max(...bounds.map((b) => b ?? 0)) || null)}
        </div>
      )}
      <div
        ref={container}
        role="img"
        aria-label={`Median delay per exchange over the last 60 minutes. Now: ${data.exchanges
          .map((e, i) => `${e.name} ${formatMs(data.y[i]?.at(-1))}`)
          .join(", ")}.`}
      />
    </div>
  );
}

/** Dashed vertical lines at each gap's start, labelled; labels that would overlap move down a line. */
function drawMarkers(u: uPlot, markers: GapMarker[]) {
  const { ctx } = u;
  const { left, top, width, height } = u.bbox;
  const ratio = window.devicePixelRatio || 1;
  ctx.save();
  ctx.font = LABEL_FONT.replace("11px", `${11 * ratio}px`);
  ctx.textBaseline = "bottom";
  // uPlot leaves the alignment of its last axis labels (right) behind.
  ctx.textAlign = "left";
  let lastRight = -Infinity;
  let row = 0;
  for (const marker of markers) {
    const x = Math.round(u.valToPos(marker.timeSec, "x", true));
    if (x < left || x > left + width) {
      continue;
    }
    ctx.strokeStyle = WARNING;
    ctx.lineWidth = ratio;
    ctx.setLineDash([3 * ratio, 3 * ratio]);
    ctx.beginPath();
    ctx.moveTo(x + 0.5, top);
    ctx.lineTo(x + 0.5, top + height);
    ctx.stroke();

    const label = `${marker.label} ${formatClock(marker.timeSec * 1000)}`;
    const textWidth = ctx.measureText(label).width;
    // Right of the line, or left of it near the right edge, and never outside the plot.
    const preferred =
      x + 4 * ratio + textWidth > left + width ? x - 4 * ratio - textWidth : x + 4 * ratio;
    const textX = Math.max(left, Math.min(preferred, left + width - textWidth));
    row = textX < lastRight + 6 * ratio ? row + 1 : 0;
    lastRight = textX + textWidth;
    ctx.fillStyle = WARNING;
    ctx.fillText(label, textX, top - 6 * ratio + row * 14 * ratio);
  }
  ctx.restore();
}
