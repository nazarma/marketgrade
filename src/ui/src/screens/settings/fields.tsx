import { useId, type ReactNode } from "react";

/** A labelled control with a hint below it, replaced by the error when there is one. */
export function Field({
  label,
  hint,
  error,
  children,
}: {
  label: string;
  hint?: string;
  error?: string;
  children: (id: string, describedBy: string) => ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      {children(id, `${id}-message`)}
      <p id={`${id}-message`} className={`m-0 ${error ? "bad text-[13px]" : "lbl"}`}>
        {error ?? hint}
      </p>
    </div>
  );
}

export function NumberField({
  label,
  unit,
  hint,
  error,
  value,
  onChange,
}: {
  label: string;
  unit: string;
  hint: string;
  error?: string;
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <Field label={label} hint={hint} error={error}>
      {(id, describedBy) => (
        <div className="flex items-center gap-2">
          <input
            id={id}
            inputMode="decimal"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            aria-invalid={error ? true : undefined}
            aria-describedby={describedBy}
            className="field-input mono w-32"
          />
          {unit && <span className="text-sm text-muted-foreground">{unit}</span>}
        </div>
      )}
    </Field>
  );
}

export function Toggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-2.5 text-sm font-medium">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="size-4 accent-[var(--mg-accent)]"
      />
      {label}
    </label>
  );
}

export type SaveState =
  { kind: "idle" } | { kind: "saving" } | { kind: "saved" } | { kind: "failed"; message: string };

/** Save, reset and what happened, under a form. */
export function FormActions({
  save,
  onSave,
  onReset,
}: {
  save: SaveState;
  onSave: () => void;
  onReset: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-3">
      <button
        type="button"
        className="btn-primary"
        onClick={onSave}
        disabled={save.kind === "saving"}
      >
        Save
      </button>
      <button type="button" className="btn-secondary" onClick={onReset}>
        Reset to defaults
      </button>
      {save.kind === "saved" && <span className="ok text-sm">Saved and applied.</span>}
      {save.kind === "failed" && <span className="bad text-sm">{save.message}</span>}
    </div>
  );
}

export function SettingsPanel({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children: ReactNode;
}) {
  return (
    <section className="panel flex max-w-[880px] flex-col gap-5 px-6 py-5" aria-label={title}>
      <div>
        <h2 className="h2">{title}</h2>
        <p className="lbl mt-1 mb-0 text-[13px]">{description}</p>
      </div>
      {children}
    </section>
  );
}
