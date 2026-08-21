import { defineScreen } from "@cord/sdk";

export default defineScreen({
  key: "pricing",
  label: "Pricing & Growth",
  icon: "tag-multiple",
  subject: "acquisition",
  layout: [
    {
      kind: "row",
      gap: "md",
      blocks: [
        {
          kind: "card",
          label: "Customers won",
          blocks: [
            {
              kind: "stat",
              label: "Across the plan",
              source: {
                entity: "acquisition",
                aggregate: {
                  op: "sum",
                  field: "new_customers",
                },
              },
            },
          ],
          grow: true,
        },
        {
          kind: "card",
          label: "Setup & services",
          blocks: [
            {
              kind: "stat",
              label: "Across the plan",
              source: {
                entity: "acquisition",
                aggregate: {
                  op: "sum",
                  field: "services_revenue",
                },
              },
              format: "money",
            },
          ],
        },
        {
          kind: "card",
          label: "Segments",
          blocks: [
            {
              kind: "stat",
              label: "Modelled",
              source: {
                entity: "segment",
                aggregate: {
                  op: "count",
                },
              },
            },
          ],
        },
      ],
    },
    {
      kind: "tabs",
      tabs: [
        {
          key: "acquisition",
          label: "Acquisition",
          blocks: [
            {
              kind: "text",
              size: "sm",
              value: "New customers won each month, by segment. This is the one table in the revenue model you type into — everything else is derived from it.",
            },
            {
              kind: "view",
              view: "acquisition_table",
            },
          ],
        },
        {
          key: "streams",
          label: "Services & other revenue",
          blocks: [
            {
              kind: "text",
              size: "sm",
              value: "Onboarding beyond the standard setup fee, custom development, consulting, white-label and marketplace. Kept out of the subscription line because none of it is recurring platform revenue.",
            },
            {
              kind: "view",
              view: "revenue_streams_table",
            },
            {
              kind: "create",
              icon: "plus",
              label: "New Revenue Stream",
              style: "primary",
              entity: "revenue_stream",
            },
          ],
        },
        {
          key: "segments",
          label: "Segments",
          blocks: [
            {
              kind: "text",
              size: "sm",
              value: "Each segment described AT MATURITY. A customer arrives small and grows into these numbers along the adoption curve.",
            },
            {
              kind: "view",
              view: "segments_table",
            },
            {
              kind: "create",
              icon: "plus",
              label: "New Segment",
              style: "primary",
              entity: "segment",
            },
          ],
        },
        {
          key: "curve",
          label: "Adoption curve",
          blocks: [
            {
              kind: "text",
              size: "sm",
              value: "How much of a customer's eventual size is live at each age. One curve for every segment. Past month twelve it goes above 1.0 — a retained customer keeps growing, which is where most of the revenue comes from.",
            },
            {
              kind: "view",
              view: "adoption_table",
            },
          ],
        },
        {
          key: "lifecycle",
          label: "Lifecycle & plan choice",
          blocks: [
            {
              kind: "text",
              size: "sm",
              value: "What each plan would cost this customer at this age, and therefore what they pay. The cheapest column wins — the plan is an outcome, not an assumption.",
            },
            {
              kind: "view",
              view: "lifecycle_table",
            },
          ],
        },
        {
          key: "price_list",
          label: "Price list",
          blocks: [
            {
              kind: "text",
              size: "sm",
              value: "Base fee, rate per active user x app, and the cap factor k. Billable users for one app stop at k x the square root of the customer's active users.",
            },
            {
              kind: "view",
              view: "pricing_table",
            },
          ],
        },
      ],
    },
  ],
} as const);
