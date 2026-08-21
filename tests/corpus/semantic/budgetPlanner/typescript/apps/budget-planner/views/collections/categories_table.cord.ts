import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "categories_table",
  label: "Cost Categories",
  entity: "cost_category",
  kind: "table",
  settings: {
    columns: ["name", "cost_group", "is_opex"],
  },
} as const);
