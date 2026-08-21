import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "team",
  blocks: [
    {
      kind: "fields",
      fields: ["name", "function", "default_salary", "description"],
      columns: 2,
    },
  ],
} as const);
