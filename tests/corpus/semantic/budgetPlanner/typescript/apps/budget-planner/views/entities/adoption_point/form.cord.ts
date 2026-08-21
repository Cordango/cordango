import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "adoption_point",
  blocks: [
    {
      kind: "fields",
      columns: 2,
      fields: ["scenario", "label", "age", "user_adoption", "apps_adoption"],
    },
  ],
} as const);
