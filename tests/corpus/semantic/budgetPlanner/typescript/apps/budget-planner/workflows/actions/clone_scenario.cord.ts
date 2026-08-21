import { defineAction } from "@cord/sdk";

export default defineAction({
  key: "clone_scenario",
  icon: "content-duplicate",
  input: {
    fields: ["name", "case_type"],
    required: ["name"],
  },
  label: "Duplicate as New Scenario",
  entity: "scenario",
  effects: [
    {
      set: {
        name: "Copy of {{record.name}}",
        owner: "{{actor.id}}",
        case_type: "{{record.case_type}}",
        plan_start: "{{record.plan_start}}",
        plan_years: "{{record.plan_years}}",
        assumptions: "{{record.assumptions}}",
        currency_code: "{{record.currency_code}}",
        starting_cash: "{{record.starting_cash}}",
        monthly_months: "{{record.monthly_months}}",
      },
      type: "createRecord",
      entity: "scenario",
    },
  ],
  description: "Create a sibling scenario to model a different case from the same starting point.",
  successMessage: "Scenario duplicated.",
} as const);
