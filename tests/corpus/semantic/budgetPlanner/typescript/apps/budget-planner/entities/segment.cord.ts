import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "segment",
  kind: "collection",
  icon: "office-building-outline",
  label: "Segment",
  description: "A band of company size, described at MATURITY. A customer arrives small and grows into these numbers along the adoption curve.",
  plural: "Segments",
  display: "name",
  fields: {
    name: {
      label: "Segment",
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
    mature_active_users: {
      label: "Mature active users",
      type: "integer",
      required: true,
      help: "Active Cordango users in the company once it is fully rolled out. This is N in the cap formula.",
    },
    mature_shared_apps: {
      label: "Mature shared apps",
      type: "decimal",
      precision: 16,
      scale: 4,
      required: true,
    },
    avg_app_adoption: {
      label: "Average app adoption",
      type: "decimal",
      precision: 16,
      scale: 4,
      help: "Share of the company's active users who use any ONE given app. Multiplied by active users to get A, the per-app population the cap bounds.",
    },
    setup_fee: {
      label: "Setup / services fee",
      type: "money",
      currency: "EUR",
      help: "One-off, invoiced in the month the customer is won.",
    },
    churn_pct: {
      label: "Churn / month",
      type: "decimal",
      unit: "%",
      precision: 8,
      scale: 2,
      default: 0,
      help: "Applied as pow(1 - rate, age - 1) along the cohort. The workbook defines this per segment and never applies it; here it does.",
    },
    notes: {
      label: "Notes",
      type: "longtext",
    },
  },
} as const);
