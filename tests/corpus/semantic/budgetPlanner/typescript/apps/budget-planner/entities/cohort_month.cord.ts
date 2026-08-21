import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "cohort_month",
  kind: "collection",
  icon: "table-large",
  label: "Cohort Month",
  description: "One cohort at one age. The grid exists because a rollup can add a field up but cannot multiply two rows together, and month m's revenue is exactly that product summed over every live cohort.",
  plural: "Cohort Grid",
  display: "label",
  fields: {
    label: {
      label: "Cell",
      type: "text",
    },
    scenario: {
      label: "Scenario",
      type: "reference",
      targetEntity: "scenario",
      required: true,
      indexed: true,
      onDelete: "cascade",
    },
    acquisition: {
      label: "Cohort",
      type: "reference",
      targetEntity: "acquisition",
      required: true,
      indexed: true,
      onDelete: "cascade",
    },
    step: {
      label: "Lifecycle step",
      type: "reference",
      targetEntity: "lifecycle_step",
      required: true,
      indexed: true,
      onDelete: "cascade",
    },
    lands_in: {
      label: "Lands in month",
      type: "integer",
      group: "Calculated",
      help: "The cohort's own month plus its age, less one. Two references, one hop each — which is the rule, and is what makes the grid legal.",
      calculate: {
        expression: "acquisition.month + step.age - 1",
      },
    },
    recurring_revenue: {
      label: "Recurring revenue",
      type: "money",
      currency: "EUR",
      group: "Calculated",
      calculate: {
        expression: "acquisition.new_customers * step.revenue_per_new_customer",
      },
    },
    surviving_customers: {
      label: "Surviving customers",
      type: "decimal",
      precision: 16,
      scale: 4,
      group: "Calculated",
      calculate: {
        expression: "acquisition.new_customers * step.survival",
      },
    },
  },
} as const);
