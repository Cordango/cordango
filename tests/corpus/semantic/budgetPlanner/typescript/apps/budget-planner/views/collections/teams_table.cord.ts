import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "teams_table",
  label: "Teams",
  entity: "team",
  kind: "table",
  settings: {
    columns: ["name", "function", "default_salary"],
  },
} as const);
