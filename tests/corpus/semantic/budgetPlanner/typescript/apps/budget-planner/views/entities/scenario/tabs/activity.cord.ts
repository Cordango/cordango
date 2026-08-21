import { defineTab } from "@cord/sdk";

export default defineTab({
  key: "activity",
  label: "Activity",
  blocks: [
    {
      kind: "history",
    },
  ],
} as const);
