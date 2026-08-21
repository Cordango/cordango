import { defineAutomation } from "@cord/sdk";

export default defineAutomation({
  key: "monthly_plan_review",
  name: "Monthly plan review reminder",
  trigger: "schedule",
  cron: "0 8 1 * *",
  entity: "scenario",
  when: {
    field: "scenario_stage",
    value: ["modelling", "review", "investor_ready"],
    operator: "in",
  },
  effects: [
    {
      to: "{{record.owner}}",
      link: "auto",
      type: "notify",
      title: "Update the budget plan: {{record.name}}",
      message: "A new month started. Enter last month's actuals and recalculate {{record.name}}.",
    },
  ],
} as const);
