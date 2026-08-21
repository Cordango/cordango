import { defineDetail } from "@cord/sdk";

export default defineDetail({
  key: "team",
  blocks: [
    {
      cols: 2,
      kind: "fields",
      fields: ["name", "function", "default_salary", "description"],
    },
    {
      kind: "table",
      label: "Hiring lines for this team",
      fields: [],
      source: {
        sort: [
          {
            field: "start_month",
            direction: "asc",
          },
        ],
        entity: "hiring_line",
        filters: [
          {
            field: "team",
            value: "{{record.id}}",
            operator: "eq",
          },
        ],
      },
    },
  ],
} as const);
