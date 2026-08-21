import { defineDetail } from "@cord/sdk";

export default defineDetail({
  key: "cost_category",
  blocks: [
    {
      cols: 2,
      kind: "fields",
      fields: ["name", "cost_group", "is_opex", "description"],
    },
    {
      kind: "table",
      label: "Cost lines in this category",
      fields: ["name"],
      source: {
        entity: "cost_line",
        filters: [
          {
            field: "cost_category",
            value: "{{record.id}}",
            operator: "eq",
          },
        ],
      },
    },
  ],
} as const);
