import { defineEntity } from "@cord/sdk";

export default defineEntity({
  key: "scenario",
  icon: "file-compare",
  kind: "collection",
  label: "Scenario",
  description: "One named version of the budget plan (e.g. Conservative, Base, Optimistic) that everything else hangs off.",
  plural: "Scenarios",
  display: "name",
  fields: {
    name: {
      type: "text",
      label: "Scenario Name",
      unique: true,
      required: true,
    },
    scenario_stage: {
      role: "status",
      type: "select",
      label: "Stage",
      indexed: true,
    },
    case_type: {
      type: "select",
      label: "Case",
      options: [
        {
          color: "#64748b",
          label: "Conservative",
          value: "conservative",
        },
        {
          color: "#0f766e",
          label: "Base Case",
          value: "base",
        },
        {
          color: "#f59e0b",
          label: "Optimistic",
          value: "optimistic",
        },
      ],
      required: true,
    },
    summary: {
      type: "longtext",
      group: "Narrative",
      label: "Investor Summary",
    },
    assumptions: {
      type: "longtext",
      group: "Narrative",
      label: "Key Assumptions",
    },
    plan_start: {
      role: "start",
      type: "date",
      group: "Horizon",
      label: "Plan Start Month",
      required: true,
    },
    plan_years: {
      type: "integer",
      unit: " yr",
      group: "Horizon",
      label: "Plan Length (years)",
      default: 5,
    },
    monthly_months: {
      help: "Number of months modelled monthly before the plan switches to yearly periods.",
      type: "integer",
      unit: " mo",
      group: "Horizon",
      label: "Monthly Detail (months)",
      default: 12,
    },
    currency_code: {
      type: "select",
      group: "Horizon",
      label: "Currency",
      default: "EUR",
      options: [
        {
          label: "Euro (EUR)",
          value: "EUR",
        },
        {
          label: "US Dollar (USD)",
          value: "USD",
        },
        {
          label: "Swiss Franc (CHF)",
          value: "CHF",
        },
        {
          label: "Pound Sterling (GBP)",
          value: "GBP",
        },
      ],
    },
    starting_cash: {
      type: "money",
      group: "Cash",
      label: "Starting Cash",
      default: 0,
      currency: "EUR",
    },
    total_funding: {
      type: "money",
      group: "Cash",
      label: "Total Funding Raised",
      currency: "EUR",
      calculate: {
        aggregate: {
          op: "sum",
          via: "scenario",
          field: "amount",
          entity: "funding_round",
        },
      },
    },
    total_revenue: {
      type: "money",
      group: "Totals",
      label: "Total Plan Revenue",
      currency: "EUR",
      calculate: {
        aggregate: {
          op: "sum",
          via: "scenario",
          field: "revenue",
          entity: "period",
        },
      },
    },
    total_costs: {
      label: "Total Plan Costs",
      type: "money",
      group: "Totals",
      currency: "EUR",
      help: "Cost of revenue, people and operating costs across every period.",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "total_cost",
        },
      },
    },
    net_result: {
      label: "Net Result",
      type: "money",
      group: "Totals",
      currency: "EUR",
      calculate: {
        expression: "total_revenue - total_costs - total_tax",
      },
    },
    cash_at_end: {
      label: "Cash At Plan End",
      type: "money",
      group: "Totals",
      currency: "EUR",
      help: "Opening cash plus everything the periods moved — the same number the last period's Cash at end shows.",
      calculate: {
        expression: "starting_cash + total_cash_movement",
      },
    },
    runway_months: {
      label: "Runway (months)",
      type: "decimal",
      group: "Totals",
      unit: " mo",
      precision: 10,
      scale: 1,
      help: "Months of cover at the plan's average monthly spend.",
      calculate: {
        expression: "cash_at_end / ((total_costs + total_tax) / period_count)",
      },
    },
    period_count: {
      type: "integer",
      group: "Totals",
      label: "Periods Modelled",
      calculate: {
        aggregate: {
          op: "count",
          via: "scenario",
          entity: "period",
        },
      },
    },
    headcount_end: {
      type: "integer",
      group: "Totals",
      label: "Planned Headcount (end)",
      calculate: {
        aggregate: {
          op: "sum",
          via: "scenario",
          field: "headcount",
          entity: "hiring_line",
        },
      },
    },
    shared_with_investors: {
      type: "boolean",
      group: "Narrative",
      label: "Shared With Investors",
      default: false,
    },
    last_reviewed_at: {
      type: "datetime",
      group: "Narrative",
      label: "Last Reviewed",
    },
    owner: {
      type: "reference",
      label: "Owner",
      targetApp: "platform",
      targetEntity: "person",
    },
    plan_months: {
      label: "Plan months",
      type: "integer",
      default: 24,
      help: "How many monthly periods to generate for this plan",
      group: "Assumptions",
    },
    total_tax: {
      label: "Total Tax Provision",
      type: "money",
      group: "Totals",
      currency: "EUR",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "tax_provision",
        },
      },
    },
    total_cash_movement: {
      label: "Net Cash Movement",
      type: "money",
      group: "Totals",
      currency: "EUR",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "net_cash_movement",
        },
      },
    },
    monthly_fixed_costs: {
      label: "Fixed costs / month",
      type: "money",
      group: "From Costs",
      help: "Summary of the Costs & Fees tab. Change it there.",
      calculate: {
        aggregate: {
          entity: "cost_line",
          via: "scenario",
          op: "sum",
          field: "monthly_amount",
        },
      },
    },
    revenue_fee_rate: {
      label: "Revenue-share fees",
      type: "decimal",
      group: "From Costs",
      help: "Summary of the Costs & Fees tab. Change it there.",
      calculate: {
        aggregate: {
          entity: "cost_line",
          via: "scenario",
          op: "sum",
          field: "percent_amount",
        },
      },
    },
    one_off_costs: {
      label: "One-off costs",
      type: "money",
      group: "From Costs",
      help: "Summary of the Costs & Fees tab. Change it there.",
      calculate: {
        aggregate: {
          entity: "cost_line",
          via: "scenario",
          op: "sum",
          field: "one_off_amount",
        },
      },
    },
    total_other_revenue: {
      label: "Services & Other Revenue",
      type: "money",
      currency: "EUR",
      group: "Calculated",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "other_revenue",
        },
      },
    },
    ai_charge_rate: {
      label: "AI billed to customers",
      type: "decimal",
      unit: "%",
      precision: 8,
      scale: 2,
      group: "AI",
      default: 6,
      help: "Share of recurring MRR billed on for AI. The workbook carries 6% as a COST; billing it on at the same rate is pure pass-through.",
    },
    ai_provider_discount: {
      label: "Provider discount",
      type: "decimal",
      unit: "%",
      precision: 8,
      scale: 2,
      group: "AI",
      default: 0,
      help: "How much less we pay the provider than we bill. This is the AI margin: we ask the customer a published rate and may buy cheaper.",
    },
    total_recurring: {
      label: "Total Recurring Revenue",
      type: "money",
      currency: "EUR",
      group: "Totals",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "recurring_mrr",
        },
      },
    },
    total_services: {
      label: "Total Services Revenue",
      type: "money",
      currency: "EUR",
      group: "Totals",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "services_revenue",
        },
      },
    },
    total_ai_margin: {
      label: "Total AI Margin",
      type: "money",
      currency: "EUR",
      group: "Totals",
      calculate: {
        aggregate: {
          entity: "period",
          via: "scenario",
          op: "sum",
          field: "ai_margin",
        },
      },
    },
    customers_won: {
      label: "Customers Won",
      type: "integer",
      group: "Totals",
      calculate: {
        aggregate: {
          entity: "acquisition",
          via: "scenario",
          op: "sum",
          field: "new_customers",
        },
      },
    },
  },
} as const);
