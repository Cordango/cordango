import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "lifecycle_table",
  label: "Lifecycle",
  entity: "lifecycle_step",
  kind: "table",
  settings: {
    columns: [
      "segment",
      "age",
      "active_users",
      "shared_apps",
      "active_per_app",
      "cost_flex",
      "cost_starter",
      "cost_pro",
      "cost_advanced",
      "cheapest_plan_cost",
      "survival",
      "revenue_per_new_customer",
    ],
    filterBar: {
      facets: ["scenario", "segment"],
    },
    defaultSort: [
      {
        field: "age",
        direction: "asc",
      },
    ],
  },
} as const);
