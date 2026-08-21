import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "period",
  blocks: [
    {
      kind: "section",
      label: "Period",
      blocks: [
        {
          kind: "fields",
          fields: ["scenario", "label", "period_type", "start_date", "end_date", "sequence"],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Users",
      blocks: [
        {
          kind: "fields",
          fields: ["active_customers", "churned_customers"],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Revenue & costs",
      blocks: [
        {
          kind: "fields",
          fields: ["revenue", "payroll_cost"],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Cash",
      blocks: [
        {
          kind: "fields",
          fields: ["cash_end"],
          columns: 2,
        },
      ],
    },
  ],
} as const);
