import { defineForm } from "@cord/sdk";

export default defineForm({
  key: "scenario",
  blocks: [
    {
      kind: "section",
      label: "Scenario",
      blocks: [
        {
          kind: "fields",
          fields: ["name", "case_type"],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Horizon",
      blocks: [
        {
          kind: "fields",
          fields: ["plan_start", "plan_years", "monthly_months", "currency_code"],
          columns: 2,
        },
      ],
    },
    {
      kind: "section",
      label: "Cash",
      blocks: [
        {
          kind: "fields",
          fields: ["starting_cash"],
          columns: 1,
        },
      ],
    },
    {
      kind: "section",
      label: "Narrative",
      blocks: [
        {
          kind: "fields",
          fields: ["summary", "assumptions"],
          columns: 1,
        },
      ],
    },
  ],
} as const);
