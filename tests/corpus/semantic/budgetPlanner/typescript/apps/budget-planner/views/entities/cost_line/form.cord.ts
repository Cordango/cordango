import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "cost_line",
  blocks: [
    {
      kind: "section",
      label: "Cost line",
      blocks: [
        {
          kind: "fields",
          fields: ["scenario", "name", "cost_category", "cost_behaviour"],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Amount",
      blocks: [
        {
          kind: "fields",
          fields: [
            "monthly_amount",
            "amount_per_user",
            "percent_amount",
            "percent_basis",
            "one_off_amount",
            "one_off_month",
            "one_off_date",
          ],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Timing & source",
      blocks: [
        {
          kind: "fields",
          fields: ["start_month", "end_month", "annual_increase_rate", "is_government_fee", "vendor", "notes"],
          columns: 2,
        },
      ],
    },
  ],
} as const);
