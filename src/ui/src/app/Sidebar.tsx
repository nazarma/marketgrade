import { Tooltip } from "radix-ui";
import type { ComponentType, ReactNode } from "react";
import { NavLink } from "react-router";
import { FlowIcon, HealthIcon, LogoMark, OverviewIcon, SettingsIcon } from "@/app/icons";
import { useMediaQuery } from "@/app/useMediaQuery";

/** Below this window width the sidebar shows icons only, with the names as tooltips. */
export const COLLAPSE_QUERY = "(max-width: 1199px)";

interface Item {
  to: string;
  label: string;
  icon: ComponentType;
}

const MAIN: Item[] = [
  { to: "/overview", label: "Overview", icon: OverviewIcon },
  { to: "/flow", label: "Live flow", icon: FlowIcon },
  { to: "/health", label: "Data health", icon: HealthIcon },
];

const SETTINGS: Item = { to: "/settings", label: "Settings", icon: SettingsIcon };

export function Sidebar() {
  const collapsed = useMediaQuery(COLLAPSE_QUERY);
  return (
    <Tooltip.Provider delayDuration={200}>
      <nav
        aria-label="Main"
        className={`sticky top-0 flex h-screen shrink-0 flex-col gap-1 border-r border-border py-5 ${
          collapsed ? "w-[68px] px-[10px]" : "w-[220px] px-[14px]"
        }`}
      >
        <div
          className={`flex items-center gap-2.5 pt-1 pb-5 ${collapsed ? "justify-center" : "px-3"}`}
        >
          <LogoMark />
          {!collapsed && <span className="text-base font-semibold">Marketgrade</span>}
        </div>
        {MAIN.map((item) => (
          <Link key={item.to} item={item} collapsed={collapsed} />
        ))}
        <div className="my-3 h-px bg-border" />
        <Link item={SETTINGS} collapsed={collapsed} />
      </nav>
    </Tooltip.Provider>
  );
}

function Link({ item, collapsed }: { item: Item; collapsed: boolean }) {
  const Icon = item.icon;
  const link = (
    <NavLink
      to={item.to}
      aria-label={collapsed ? item.label : undefined}
      className={({ isActive }) =>
        `flex min-h-6 items-center gap-2.5 rounded-lg py-2.5 text-sm no-underline ${
          collapsed ? "justify-center px-0" : "px-3"
        } ${isActive ? "bg-raised text-foreground" : "text-muted-foreground hover:bg-raised hover:text-foreground"}`
      }
    >
      <Icon />
      {!collapsed && item.label}
    </NavLink>
  );
  return collapsed ? <WithTooltip label={item.label}>{link}</WithTooltip> : link;
}

function WithTooltip({ label, children }: { label: string; children: ReactNode }) {
  return (
    <Tooltip.Root>
      <Tooltip.Trigger asChild>{children}</Tooltip.Trigger>
      <Tooltip.Portal>
        <Tooltip.Content
          side="right"
          sideOffset={8}
          className="rounded-md border border-border bg-raised px-2.5 py-1.5 text-xs text-foreground"
        >
          {label}
        </Tooltip.Content>
      </Tooltip.Portal>
    </Tooltip.Root>
  );
}
