import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "periods_table",
  label: "All Periods",
  entity: "period",
  kind: "table",
  settings: {
    columns: [
      "label",
      "start_date",
      "new_customers",
      "active_customers",
      "recurring_mrr",
      "services_revenue",
      "enterprise_mrr",
      "ai_charge",
      "revenue",
      "total_cost",
      "cash_end",
    ],
    filterBar: {
      facets: ["scenario", "period_type"],
      search: ["label"],
    },
    inlineEdit: true,
    defaultSort: [
      {
        field: "sequence",
        direction: "asc",
      },
    ],
  },
} as const);
