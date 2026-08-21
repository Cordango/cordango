import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "revenue_stream",
  blocks: [
    {
      kind: "section",
      label: "Stream",
      blocks: [
        {
          kind: "fields",
          columns: 2,
          fields: ["name", "scenario", "stream_type", "revenue_behaviour"],
        },
      ],
    },
    {
      kind: "section",
      label: "Amounts",
      blocks: [
        {
          kind: "fields",
          columns: 2,
          fields: ["monthly_amount", "one_off_amount", "one_off_month", "one_off_date", "creator_share_pct"],
        },
      ],
    },
    {
      kind: "section",
      label: "Timing",
      blocks: [
        {
          kind: "fields",
          columns: 2,
          fields: ["start_month", "end_month"],
        },
      ],
    },
    {
      kind: "section",
      label: "Notes",
      blocks: [
        {
          kind: "fields",
          columns: 1,
          fields: ["customer", "notes"],
        },
      ],
    },
  ],
} as const);
