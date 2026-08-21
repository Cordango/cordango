import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "lifecycle_step",
  blocks: [
    {
      kind: "fields",
      columns: 2,
      fields: ["scenario", "segment", "point", "label"],
    },
  ],
} as const);
