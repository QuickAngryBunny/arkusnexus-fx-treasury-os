"use client";

import { Segmented, Switch } from "@fxos/ui";

/** Interactive samples for the /ui reference page. */
export function UiDemoControls({ rangeLabel, switchLabel, switchNote }: { rangeLabel: string; switchLabel: string; switchNote: string }) {
  const range = [
    { value: "7d", label: "7D" },
    { value: "15d", label: "15D" },
    { value: "30d", label: "30D" },
    { value: "90d", label: "90D" },
  ] as const;
  return (
    <div className="grid">
      <div className="stack-sm">
        <Segmented label={rangeLabel} options={range} defaultValue="30d" onWhite />
      </div>
      <Switch label={switchLabel} note={switchNote} defaultChecked />
      <Switch label={switchLabel} note={switchNote} />
    </div>
  );
}
