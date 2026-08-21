import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "cost_lines_table",
  label: "Cost Lines",
  entity: "cost_line",
  kind: "table",
  settings: {
    columns: [
      "name",
      "cost_category",
      "cost_behaviour",
      "monthly_amount",
      "amount_per_user",
      "percent_amount",
      "percent_basis",
      "one_off_amount",
      "one_off_month",
      "one_off_date",
      "is_government_fee",
    ],
    filterBar: {
      facets: ["scenario", "cost_category", "cost_behaviour"],
      search: ["name", "vendor"],
    },
    inlineEdit: true,
    allowDelete: true,
  },
} as const);
