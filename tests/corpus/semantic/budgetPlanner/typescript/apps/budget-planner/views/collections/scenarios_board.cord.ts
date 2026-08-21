import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "scenarios_board",
  label: "By Stage",
  entity: "scenario",
  kind: "kanban",
  settings: {
    cardFields: ["case_type", "total_revenue", "cash_at_end", "runway_months"],
    interaction: "interactive",
    groupByField: "scenario_stage",
  },
} as const);
