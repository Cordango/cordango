import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "growth_phase",
  blocks: [
    {
      kind: "fields",
      columns: 2,
      fields: ["scenario", "name", "from_month", "to_month", "growth_pct", "churn_pct", "notes"],
    },
  ],
} as const);
