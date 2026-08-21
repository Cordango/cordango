import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "revenue_plan",
  icon: "tag-multiple",
  kind: "collection",
  label: "Plan",
  description: "One row of the published price list: a base fee, a rate per active user x app, and the cap factor k. Nothing here says who buys it — a customer lands on whichever plan is cheapest for their shape, so the plan is an outcome.",
  plural: "Plans",
  display: "name",
  fields: {
    name: {
      type: "text",
      label: "Plan Name",
      required: true,
    },
    scenario: {
      type: "reference",
      label: "Scenario",
      indexed: true,
      onDelete: "cascade",
      required: true,
      targetEntity: "scenario",
    },
    tier: {
      type: "select",
      label: "Tier",
      options: [
        {
          value: "flex",
          label: "Flex / Free",
          color: "#94a3b8",
        },
        {
          value: "starter",
          label: "Starter",
          color: "#38bdf8",
        },
        {
          value: "pro",
          label: "Pro",
          color: "#0f766e",
        },
        {
          value: "advanced",
          label: "Advanced",
          color: "#6366f1",
        },
        {
          value: "enterprise",
          label: "Enterprise",
          color: "#f59e0b",
        },
      ],
      required: true,
    },
    monthly_base_fee: {
      label: "Base fee / month",
      type: "money",
      currency: "EUR",
      group: "Price",
      default: 0,
      help: "Charged whatever the usage is. Zero on Flex.",
    },
    price_per_app_user: {
      label: "Rate / active user x app",
      type: "money",
      currency: "EUR",
      group: "Price",
      help: "One unit is one person actively using one shared app this month.",
    },
    cap_multiplier: {
      label: "Cap factor k",
      type: "decimal",
      precision: 16,
      scale: 4,
      group: "Price",
      help: "Billable users for ONE app stop at k x the square root of the customer's active users. Blank on Flex (no cap) and on Enterprise (negotiated).",
    },
    notes: {
      type: "longtext",
      group: "Unit Economics",
      label: "Notes",
    },
  },
} as const);
