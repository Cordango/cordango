import { defineAutomation } from "@cord/sdk";

export default defineAutomation({
  key: "generate_periods",
  name: "Lay out the plan's months",
  trigger: "record.created",
  entity: "scenario",
  effects: [
    {
      type: "createForEach",
      entity: "period",
      source: {
        range: {
          from: "{{record.plan_start}}",
          count: "{{record.plan_months}}",
          step: "month",
        },
      },
      key: ["scenario", "sequence"],
      set: {
        label: "Month {{source.index}}",
        scenario: "{{record.id}}",
        sequence: "{{source.index}}",
        start_date: "{{source.date}}",
        period_type: "month",
        end_date: "{{source.end}}",
      },
    },
  ],
} as const);
