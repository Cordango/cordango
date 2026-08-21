import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "cost_category",
  blocks: [
    {
      kind: "fields",
      fields: ["name", "cost_group", "is_opex", "description"],
      columns: 2,
    },
  ],
} as const);
