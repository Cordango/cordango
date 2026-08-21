import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "acquisition_table",
  label: "New customers by month",
  entity: "acquisition",
  kind: "table",
  settings: {
    columns: ["month", "segment", "new_customers", "services_revenue"],
    filterBar: {
      facets: ["scenario", "segment"],
      search: ["label"],
    },
    inlineEdit: true,
    defaultSort: [
      {
        field: "month",
        direction: "asc",
      },
    ],
  },
} as const);
