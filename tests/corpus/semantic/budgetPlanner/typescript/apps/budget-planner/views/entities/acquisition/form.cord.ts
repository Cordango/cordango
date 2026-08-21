import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "acquisition",
  blocks: [
    {
      kind: "fields",
      columns: 2,
      fields: ["scenario", "segment", "label", "month", "new_customers"],
    },
  ],
} as const);
