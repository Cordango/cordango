import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "home",
  label: "Investor Overview",
  icon: "presentation",
  subject: "scenario",
  layout: [
    {
      gap: "md",
      kind: "row",
      blocks: [
        {
          kind: "card",
          label: "Scenarios",
          blocks: [
            {
              icon: "file-compare",
              kind: "stat",
              label: "Modelled",
              source: {
                entity: "scenario",
                aggregate: {
                  op: "count",
                },
              },
            },
          ],
        },
        {
          grow: true,
          kind: "card",
          label: "Investor-ready plan revenue",
          blocks: [
            {
              icon: "cash",
              kind: "stat",
              label: "Total revenue (investor ready)",
              format: "money",
              source: {
                entity: "scenario",
                filters: [
                  {
                    field: "scenario_stage",
                    value: "investor_ready",
                    operator: "eq",
                  },
                ],
                aggregate: {
                  op: "sum",
                  field: "total_revenue",
                },
              },
            },
          ],
        },
        {
          kind: "card",
          label: "Services & other revenue",
          blocks: [
            {
              icon: "handshake-outline",
              kind: "stat",
              label: "Onboarding, custom dev, marketplace",
              format: "money",
              source: {
                entity: "scenario",
                filters: [
                  {
                    field: "scenario_stage",
                    value: "investor_ready",
                    operator: "eq",
                  },
                ],
                aggregate: {
                  op: "sum",
                  field: "total_other_revenue",
                },
              },
            },
          ],
        },
        {
          kind: "card",
          label: "Funding",
          blocks: [
            {
              icon: "bank-transfer-in",
              kind: "stat",
              label: "Closed",
              format: "money",
              source: {
                entity: "funding_round",
                filters: [
                  {
                    field: "round_stage",
                    value: "closed",
                    operator: "eq",
                  },
                ],
                aggregate: {
                  op: "sum",
                  field: "amount",
                },
              },
            },
          ],
        },
        {
          kind: "card",
          label: "Planned headcount",
          blocks: [
            {
              icon: "account-multiple",
              kind: "stat",
              label: "Heads in hiring plan",
              source: {
                entity: "hiring_line",
                filters: [
                  {
                    field: "hire_status",
                    value: ["dropped"],
                    operator: "notIn",
                  },
                ],
                aggregate: {
                  op: "sum",
                  field: "headcount",
                },
              },
            },
          ],
        },
      ],
    },
    {
      icon: "file-compare",
      kind: "card",
      label: "Compare scenarios",
      blocks: [
        {
          gap: "sm",
          kind: "repeat",
          blocks: [
            {
              gap: "md",
              kind: "row",
              align: "center",
              blocks: [
                {
                  gap: "none",
                  kind: "stack",
                  width: "md",
                  blocks: [
                    {
                      kind: "field",
                      field: "name",
                      weight: "bold",
                    },
                    {
                      kind: "chip",
                      field: "case_type",
                    },
                  ],
                },
                {
                  kind: "chip",
                  field: "scenario_stage",
                },
                {
                  kind: "stat",
                  size: "sm",
                  field: "total_revenue",
                  label: "Revenue",
                  format: "money",
                },
                {
                  kind: "stat",
                  size: "sm",
                  field: "total_costs",
                  label: "Costs",
                  format: "money",
                },
                {
                  kind: "stat",
                  size: "sm",
                  field: "net_result",
                  label: "Net",
                  format: "money",
                },
                {
                  kind: "stat",
                  size: "sm",
                  field: "cash_at_end",
                  label: "Cash at end",
                  format: "money",
                },
                {
                  grow: true,
                  kind: "stat",
                  size: "sm",
                  field: "runway_months",
                  label: "Runway (mo)",
                },
              ],
              padding: "sm",
              bordered: true,
            },
          ],
          source: {
            sort: [
              {
                field: "case_type",
                direction: "asc",
              },
            ],
            limit: 6,
            entity: "scenario",
            filters: [
              {
                field: "scenario_stage",
                value: "archived",
                operator: "neq",
              },
            ],
          },
          emptyText: "No scenarios yet — create your first plan.",
        },
        {
          icon: "plus",
          kind: "create",
          label: "New Scenario",
          style: "primary",
          entity: "scenario",
        },
      ],
    },
    {
      kind: "columns",
      columns: [
        [
          {
            icon: "chart-donut",
            kind: "card",
            label: "Cost lines by behaviour",
            blocks: [
              {
                kind: "chart",
                source: {
                  entity: "cost_line",
                  aggregate: {
                    op: "count",
                    groupBy: "cost_behaviour",
                  },
                },
                chartType: "donut",
              },
            ],
          },
        ],
        [
          {
            icon: "bank-transfer-in",
            kind: "card",
            label: "Next funding milestones",
            blocks: [
              {
                kind: "repeat",
                blocks: [
                  {
                    gap: "sm",
                    kind: "row",
                    align: "center",
                    blocks: [
                      {
                        grow: true,
                        kind: "field",
                        field: "name",
                      },
                      {
                        kind: "chip",
                        field: "round_type",
                      },
                      {
                        kind: "field",
                        field: "amount",
                        format: "money",
                      },
                      {
                        kind: "field",
                        field: "expected_close",
                        format: "date",
                      },
                    ],
                  },
                ],
                source: {
                  sort: [
                    {
                      field: "expected_close",
                      direction: "asc",
                    },
                  ],
                  limit: 5,
                  entity: "funding_round",
                  filters: [
                    {
                      field: "round_stage",
                      value: ["closed", "abandoned"],
                      operator: "notIn",
                    },
                  ],
                },
                emptyText: "No open rounds planned.",
              },
            ],
          },
        ],
      ],
      weights: [3, 2],
    },
  ],
} as const);
