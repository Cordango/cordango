import { defineDetail } from "@cord/sdk";

export default defineDetail({
  key: "revenue_stream",
  blocks: [
    {
      kind: "hub",
      subtitle: ["stream_type", "scenario"],
      actions: ["edit", "delete"],
      facts: ["net_monthly_amount", "net_one_off_amount", "start_month", "end_month"],
    },
    {
      kind: "section",
      label: "Amounts",
      edit: [
        "revenue_behaviour",
        "monthly_amount",
        "one_off_amount",
        "one_off_month",
        "one_off_date",
        "creator_share_pct",
      ],
      blocks: [
        {
          kind: "fields",
          cols: 3,
          fields: [
            "revenue_behaviour",
            "monthly_amount",
            "one_off_amount",
            "one_off_month",
            "one_off_date",
            "creator_share_pct",
            "net_monthly_amount",
            "net_one_off_amount",
          ],
        },
      ],
    },
    {
      kind: "section",
      label: "Timing",
      edit: ["start_month", "end_month"],
      blocks: [
        {
          kind: "fields",
          cols: 2,
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
          cols: 1,
          fields: ["customer", "notes"],
        },
      ],
    },
  ],
} as const);
