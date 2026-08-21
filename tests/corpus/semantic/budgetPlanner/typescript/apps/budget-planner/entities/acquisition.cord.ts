import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "acquisition",
  kind: "collection",
  icon: "account-plus-outline",
  label: "Acquisition",
  description: "New customers won in one month, in one segment. This is the one table in the revenue model somebody types into.",
  plural: "Acquisition",
  display: "label",
  fields: {
    label: {
      label: "Month",
      type: "text",
      required: true,
    },
    scenario: {
      label: "Scenario",
      type: "reference",
      targetEntity: "scenario",
      required: true,
      indexed: true,
      onDelete: "cascade",
    },
    segment: {
      label: "Segment",
      type: "reference",
      targetEntity: "segment",
      required: true,
      indexed: true,
      onDelete: "cascade",
    },
    month: {
      label: "Plan month",
      type: "integer",
      required: true,
      help: "Counted from the plan start. Month 1 is the first month.",
    },
    new_customers: {
      label: "New customers",
      type: "integer",
      default: 0,
      help: "Typed. Everything downstream of it is derived.",
    },
    services_revenue: {
      label: "Setup & services",
      type: "money",
      currency: "EUR",
      group: "Calculated",
      help: "Invoiced once, in the month the customers are won.",
      calculate: {
        expression: "new_customers * segment.setup_fee",
      },
    },
  },
} as const);
