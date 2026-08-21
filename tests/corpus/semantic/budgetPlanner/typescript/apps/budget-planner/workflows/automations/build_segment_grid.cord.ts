import { defineAutomation } from "@cord/sdk";

export default defineAutomation({
  key: "build_segment_grid",
  name: "Lay out a segment's lifecycle",
  trigger: "record.created",
  entity: "segment",
  effects: [
    {
      type: "createForEach",
      entity: "lifecycle_step",
      source: {
        entity: "adoption_point",
        filters: [
          {
            field: "scenario",
            operator: "eq",
            value: "{{record.scenario}}",
          },
        ],
      },
      key: ["segment", "point"],
      set: {
        scenario: "{{record.scenario}}",
        segment: "{{record.id}}",
        point: "{{source.id}}",
        label: "{{record.name}} {{source.label}}",
        flex: {
          pick: {
            entity: "revenue_plan",
            filters: [
              {
                field: "scenario",
                operator: "eq",
                value: "{{record.scenario}}",
              },
              {
                field: "tier",
                operator: "eq",
                value: "flex",
              },
            ],
          },
        },
        starter: {
          pick: {
            entity: "revenue_plan",
            filters: [
              {
                field: "scenario",
                operator: "eq",
                value: "{{record.scenario}}",
              },
              {
                field: "tier",
                operator: "eq",
                value: "starter",
              },
            ],
          },
        },
        pro: {
          pick: {
            entity: "revenue_plan",
            filters: [
              {
                field: "scenario",
                operator: "eq",
                value: "{{record.scenario}}",
              },
              {
                field: "tier",
                operator: "eq",
                value: "pro",
              },
            ],
          },
        },
        advanced: {
          pick: {
            entity: "revenue_plan",
            filters: [
              {
                field: "scenario",
                operator: "eq",
                value: "{{record.scenario}}",
              },
              {
                field: "tier",
                operator: "eq",
                value: "advanced",
              },
            ],
          },
        },
      },
    },
  ],
} as const);
