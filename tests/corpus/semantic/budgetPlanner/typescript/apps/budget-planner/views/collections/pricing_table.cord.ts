import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "pricing_table",
  label: "Price list",
  entity: "revenue_plan",
  kind: "table",
  settings: {
    columns: ["name", "tier", "monthly_base_fee", "price_per_app_user", "cap_multiplier", "notes"],
    filterBar: {
      facets: ["scenario", "tier"],
      search: ["name"],
    },
    inlineEdit: true,
  },
} as const);
