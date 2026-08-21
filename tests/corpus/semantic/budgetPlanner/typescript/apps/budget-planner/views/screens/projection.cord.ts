import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "projection",
  label: "Projection",
  icon: "table-large",
  subject: "period",
  layout: [
    {
      icon: "table-large",
      kind: "card",
      label: "Plan by period",
      blocks: [
        {
          kind: "text",
          size: "sm",
          value: "Edit users, revenue and cost figures period by period. Year-1 rows are monthly; later rows are yearly.",
        },
        {
          kind: "view",
          view: "periods_table",
        },
        {
          icon: "plus",
          kind: "create",
          label: "New Period",
          style: "primary",
          entity: "period",
        },
      ],
    },
    {
      kind: "columns",
      weights: [1, 1],
      columns: [
        [
          {
            kind: "card",
            icon: "chart-line",
            label: "Revenue by month",
            blocks: [
              {
                kind: "chart",
                chartType: "bar",
                sources: [
                  {
                    label: "Recurring MRR",
                    color: "#0f766e",
                    source: {
                      entity: "period",
                      aggregate: {
                        op: "sum",
                        field: "recurring_mrr",
                        groupBy: "month_of:start_date",
                      },
                    },
                  },
                  {
                    label: "Services",
                    color: "#f59e0b",
                    source: {
                      entity: "period",
                      aggregate: {
                        op: "sum",
                        field: "services_revenue",
                        groupBy: "month_of:start_date",
                      },
                    },
                  },
                ],
              },
            ],
          },
        ],
        [
          {
            kind: "card",
            icon: "chart-areaspline",
            label: "Total costs by period",
            blocks: [
              {
                kind: "chart",
                chartType: "area",
                source: {
                  entity: "period",
                  aggregate: {
                    op: "sum",
                    field: "total_cost",
                    groupBy: "month_of:start_date",
                  },
                },
              },
            ],
          },
        ],
      ],
    },
    {
      kind: "card",
      icon: "account-group",
      label: "Active customers",
      blocks: [
        {
          kind: "chart",
          chartType: "area",
          source: {
            entity: "period",
            aggregate: {
              op: "sum",
              field: "active_customers",
              groupBy: "month_of:start_date",
            },
          },
        },
      ],
    },
  ],
} as const);
