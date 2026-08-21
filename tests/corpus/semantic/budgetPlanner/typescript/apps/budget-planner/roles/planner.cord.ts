import { defineRole } from "@cord/sdk";

export default defineRole({
  key: "planner",
  name: "Finance Planner",
  description: "Builds and maintains the plan but cannot archive scenarios or change app settings.",
  grants: {
    scenario: {
      read: true,
      create: true,
      update: true,
      commands: ["start_modelling", "submit_for_review", "clone_scenario", "request_changes"],
    },
    period: {
      read: true,
      create: true,
      delete: true,
      update: true,
      commands: ["copy_actuals"],
    },
    revenue_plan: {
      read: true,
      create: true,
      delete: true,
      update: true,
    },
    hiring_line: {
      read: true,
      create: true,
      delete: true,
      update: true,
      commands: [
        "approve_hire",
        "open_recruiting",
        "mark_filled",
        "hold_hire",
        "drop_hire",
        "shift_hire_start",
      ],
    },
    cost_line: {
      read: true,
      create: true,
      delete: true,
      update: true,
    },
    revenue_stream: {
      read: true,
      create: true,
      update: true,
      delete: true,
    },
    funding_round: {
      read: true,
      create: true,
      update: true,
      commands: ["start_raise", "record_term_sheet", "close_round", "abandon_round"],
    },
    assumption: {
      read: true,
      create: true,
      delete: true,
      update: true,
    },
    team: {
      read: true,
      create: true,
      update: true,
    },
    cost_category: {
      read: true,
      create: true,
      update: true,
    },
    budget_settings: {
      read: true,
    },
    segment: {
      read: true,
      create: true,
      update: true,
      delete: true,
    },
    adoption_point: {
      read: true,
      create: true,
      update: true,
      delete: true,
    },
    acquisition: {
      read: true,
      create: true,
      update: true,
      delete: true,
    },
    lifecycle_step: {
      read: true,
      create: true,
      update: true,
      delete: true,
    },
    cohort_month: {
      read: true,
      create: true,
      update: true,
      delete: true,
    },
  },
} as const);
