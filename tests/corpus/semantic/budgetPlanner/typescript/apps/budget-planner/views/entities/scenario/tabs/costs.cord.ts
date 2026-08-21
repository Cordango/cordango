import { defineTab } from "@cord/sdk";

export default defineTab({
  key: "costs",
  label: "Costs & Fees",
  blocks: [
    {
      via: "scenario",
      kind: "child",
      label: "Cost Lines",
      entity: "cost_line",
      fields: [
        "name",
        "cost_behaviour",
        "monthly_amount",
        "amount_per_user",
        "percent_amount",
        "percent_basis",
        "one_off_amount",
        "one_off_month",
        "one_off_date",
        "start_month",
        "end_month",
        "is_government_fee",
      ],
      groupBy: {
        field: "cost_behaviour",
      },
      childType: "table",
      inlineEdit: true,
      allowDelete: true,
      inlineCreate: true,
    },
  ],
} as const);
