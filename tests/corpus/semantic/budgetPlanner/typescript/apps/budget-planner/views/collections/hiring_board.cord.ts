import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "hiring_board",
  label: "By Team",
  entity: "hiring_line",
  kind: "kanban",
  settings: {
    cardFields: ["role_title", "headcount", "start_month", "annual_cost"],
    interaction: "interactive",
    groupByField: "team",
  },
} as const);
