import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "assumption",
  blocks: [
    {
      kind: "fields",
      fields: ["statement", "driver", "value_text", "confidence", "rationale"],
      columns: 2,
    },
  ],
} as const);
