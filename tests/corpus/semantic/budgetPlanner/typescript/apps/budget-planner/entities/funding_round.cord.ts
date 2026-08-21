import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "funding_round",
  icon: "bank-transfer-in",
  kind: "collection",
  label: "Funding Round",
  description: "Money coming in from investors within a scenario.",
  plural: "Funding",
  display: "name",
  fields: {
    name: {
      type: "text",
      label: "Round Name",
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
    round_stage: {
      role: "status",
      type: "select",
      label: "Round Status",
      indexed: true,
    },
    round_type: {
      type: "select",
      label: "Round Type",
      options: [
        {
          color: "#94a3b8",
          label: "Founder / Bootstrap",
          value: "bootstrap",
        },
        {
          color: "#0f766e",
          label: "Grant / Public Funding",
          value: "grant",
        },
        {
          color: "#38bdf8",
          label: "Angel",
          value: "angel",
        },
        {
          color: "#6366f1",
          label: "Pre-Seed",
          value: "pre_seed",
        },
        {
          color: "#f59e0b",
          label: "Seed",
          value: "seed",
        },
        {
          color: "#dc2626",
          label: "Series A",
          value: "series_a",
        },
        {
          color: "#a16207",
          label: "Loan / Debt",
          value: "debt",
        },
      ],
      required: true,
    },
    amount: {
      type: "money",
      label: "Amount",
      currency: "EUR",
      required: true,
    },
    expected_close: {
      role: "due",
      type: "date",
      label: "Expected Close",
      required: true,
    },
    pre_money_valuation: {
      type: "money",
      group: "Terms",
      label: "Pre-money Valuation",
      currency: "EUR",
    },
    equity_given: {
      type: "decimal",
      unit: "%",
      group: "Terms",
      label: "Equity Given",
      scale: 2,
      precision: 6,
    },
    lead_investor: {
      type: "text",
      group: "Terms",
      label: "Lead Investor",
    },
    investor_contact_email: {
      type: "email",
      group: "Terms",
      label: "Investor Contact",
    },
    notes: {
      type: "longtext",
      group: "Terms",
      label: "Notes",
    },
  },
} as const);
