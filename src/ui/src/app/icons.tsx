import type { SVGProps } from "react";

/** The icons of the designs, drawn with the current text colour. */
type IconProps = SVGProps<SVGSVGElement> & { size?: number };

function Icon({ size = 16, children, ...props }: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      {...props}
    >
      {children}
    </svg>
  );
}

export function LogoMark() {
  return (
    <svg
      width={22}
      height={22}
      viewBox="0 0 24 24"
      fill="none"
      stroke="var(--mg-accent)"
      strokeWidth={2}
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M12 2l9 10-9 10-9-10z" />
      <path d="M7 12h3l2-4 2 8 2-4h1" />
    </svg>
  );
}

export function OverviewIcon(props: IconProps) {
  return (
    <Icon {...props} strokeLinecap="butt" strokeLinejoin="miter">
      <rect x="3" y="3" width="7" height="7" rx="1" />
      <rect x="14" y="3" width="7" height="7" rx="1" />
      <rect x="3" y="14" width="7" height="7" rx="1" />
      <rect x="14" y="14" width="7" height="7" rx="1" />
    </Icon>
  );
}

export function FlowIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M4 6h10M4 12h16M4 18h7" />
    </Icon>
  );
}

export function HealthIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M3 12h4l3-8 4 16 3-8h4" />
    </Icon>
  );
}

export function SettingsIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <circle cx="12" cy="12" r="3" />
      <path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9L7 7M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1" />
    </Icon>
  );
}

export function CheckIcon(props: IconProps) {
  return (
    <Icon size={12} strokeWidth={3} {...props}>
      <path d="M5 12l5 5 9-10" />
    </Icon>
  );
}

export function AlertIcon(props: IconProps) {
  return (
    <Icon size={12} strokeWidth={2.5} {...props}>
      <path d="M12 4l9 16H3z" />
      <path d="M12 10v4M12 17.5v.5" />
    </Icon>
  );
}

export function CrossIcon(props: IconProps) {
  return (
    <Icon size={12} strokeWidth={3} {...props}>
      <path d="M6 6l12 12M18 6L6 18" />
    </Icon>
  );
}

export function SyncIcon(props: IconProps) {
  return (
    <Icon size={12} strokeWidth={2.5} {...props}>
      <path d="M20 12a8 8 0 0 1-14 5.3M4 12a8 8 0 0 1 14-5.3" />
      <path d="M18 3v4h-4M6 21v-4h4" />
    </Icon>
  );
}

export function PauseIcon(props: IconProps) {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="currentColor"
      aria-hidden="true"
      {...props}
    >
      <rect x="6" y="5" width="4" height="14" rx="1" />
      <rect x="14" y="5" width="4" height="14" rx="1" />
    </svg>
  );
}

export function PlayIcon(props: IconProps) {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="currentColor"
      aria-hidden="true"
      {...props}
    >
      <path d="M7 5l12 7-12 7z" />
    </svg>
  );
}

export function FullScreenIcon(props: IconProps) {
  return (
    <Icon {...props} strokeLinejoin="miter">
      <path d="M4 4h6v6H4zM14 14h6v6h-6zM10 7h7v7" />
    </Icon>
  );
}
