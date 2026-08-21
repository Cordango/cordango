import { defineLifecycle } from "@cord/sdk";

export default defineLifecycle({
  key: "scenario_lifecycle",
  entity: "scenario",
  stateField: "scenario_stage",
  initial: "draft",
  states: {
    draft: {
      color: "#94a3b8",
      label: "Draft",
      phase: "not_started",
    },
    modelling: {
      color: "#38bdf8",
      label: "Modelling",
      phase: "active",
    },
    review: {
      color: "#6366f1",
      label: "Internal Review",
      phase: "active",
    },
    investor_ready: {
      color: "#0f766e",
      label: "Investor Ready",
      phase: "done",
    },
    archived: {
      color: "#64748b",
      label: "Archived",
      phase: "cancelled",
      terminal: true,
    },
  },
  transitions: {
    start_modelling: {
      to: "modelling",
      from: ["draft"],
      label: "Start Modelling",
      action: {
        icon: "pencil-ruler",
        style: "primary",
        effects: [],
        successMessage: "Scenario moved into modelling.",
      },
    },
    submit_for_review: {
      to: "review",
      from: ["modelling"],
      label: "Submit for Review",
      action: {
        icon: "send-check",
        effects: [
          {
            set: {
              last_reviewed_at: "{{now}}",
            },
            type: "updateRecord",
          },
        ],
        successMessage: "Scenario submitted for review.",
      },
    },
    request_changes: {
      to: "modelling",
      from: ["review"],
      label: "Request Changes",
      action: {
        icon: "undo-variant",
        input: {
          fields: ["assumptions"],
        },
        effects: [
          {
            to: "{{record.owner}}",
            link: "auto",
            type: "notify",
            title: "Changes requested on {{record.name}}",
            message: "{{actor.name}} asked for changes on the budget scenario {{record.name}}.",
          },
        ],
        successMessage: "Changes requested.",
      },
    },
    mark_investor_ready: {
      to: "investor_ready",
      from: ["review", "modelling"],
      label: "Mark Investor Ready",
      action: {
        icon: "presentation",
        emits: ["scenario.investor_ready"],
        style: "primary",
        confirm: {
          title: "Mark this scenario investor ready?",
          message: "The scenario will be flagged as the version you show investors.",
          confirmLabel: "Mark ready",
        },
        effects: [
          {
            set: {
              last_reviewed_at: "{{now}}",
              shared_with_investors: true,
            },
            type: "updateRecord",
          },
        ],
        successMessage: "Scenario marked investor ready.",
      },
    },
    reopen_scenario: {
      to: "modelling",
      from: ["investor_ready"],
      label: "Reopen",
      action: {
        icon: "lock-open-variant",
        label: "Reopen for Editing",
        effects: [
          {
            set: {
              shared_with_investors: false,
            },
            type: "updateRecord",
          },
        ],
        successMessage: "Scenario reopened.",
      },
    },
    archive_scenario: {
      to: "archived",
      from: ["draft", "modelling", "review", "investor_ready"],
      label: "Archive",
      action: {
        icon: "archive",
        label: "Archive Scenario",
        style: "danger",
        confirm: {
          tone: "danger",
          title: "Archive this scenario?",
          message: "Archived scenarios stay readable but are no longer maintained.",
          confirmLabel: "Archive",
        },
        effects: [],
        successMessage: "Scenario archived.",
      },
    },
  },
} as const);
