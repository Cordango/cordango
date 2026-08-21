import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "hiring_timeline",
  label: "Hiring Timeline",
  entity: "hiring_line",
  kind: "timeline",
  settings: {
    groupBy: "team",
    dateField: "start_month",
  },
} as const);
