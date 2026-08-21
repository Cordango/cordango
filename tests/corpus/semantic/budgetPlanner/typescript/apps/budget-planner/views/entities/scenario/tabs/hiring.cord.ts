import { defineTab } from "@cord/sdk";

export default defineTab({
  key: "hiring",
  label: "Hiring",
  blocks: [
    {
      via: "scenario",
      kind: "child",
      label: "Hiring Plan",
      entity: "hiring_line",
      fields: [
        "role_title",
        "team",
        "hire_status",
        "headcount",
        "seniority",
        "start_month",
        "gross_salary",
        "employer_cost_rate",
        "setup_cost_per_head",
        "annual_cost",
      ],
      groupBy: {
        field: "team",
      },
      childType: "table",
      inlineEdit: true,
      inlineCreate: true,
    },
    {
      kind: "card",
      label: "Annual loaded cost by team",
      blocks: [
        {
          kind: "chart",
          source: {
            via: "scenario",
            entity: "hiring_line",
            aggregate: {
              op: "sum",
              field: "annual_cost",
              groupBy: "team",
            },
          },
          chartType: "bar",
        },
      ],
    },
  ],
} as const);
