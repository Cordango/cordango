import { defineLifecycle } from "@cord/sdk";

export default defineLifecycle({
  key: "hiring_lifecycle",
  entity: "hiring_line",
  stateField: "hire_status",
  initial: "planned",
  states: {
    planned: {
      color: "#94a3b8",
      label: "Planned",
      phase: "not_started",
    },
    approved: {
      color: "#38bdf8",
      label: "Approved to Hire",
      phase: "active",
    },
    recruiting: {
      color: "#6366f1",
      label: "Recruiting",
      phase: "active",
    },
    filled: {
      color: "#0f766e",
      label: "Filled",
      phase: "done",
    },
    on_hold: {
      color: "#f59e0b",
      label: "On Hold",
      phase: "not_started",
    },
    dropped: {
      color: "#dc2626",
      label: "Dropped",
      phase: "cancelled",
      terminal: true,
    },
  },
  transitions: {
    approve_hire: {
      to: "approved",
      from: ["planned", "on_hold"],
      label: "Approve to Hire",
      action: {
        icon: "check-decagram",
        style: "primary",
        effects: [],
        successMessage: "Role approved.",
      },
    },
    open_recruiting: {
      to: "recruiting",
      from: ["approved"],
      label: "Start Recruiting",
      action: {
        icon: "account-search",
        effects: [],
        successMessage: "Recruiting started.",
      },
    },
    mark_filled: {
      to: "filled",
      from: ["recruiting", "approved"],
      label: "Mark Filled",
      action: {
        icon: "account-check",
        input: {
          fields: ["start_month"],
          required: ["start_month"],
        },
        effects: [],
        successMessage: "Role marked filled.",
      },
    },
    hold_hire: {
      to: "on_hold",
      from: ["planned", "approved", "recruiting"],
      label: "Put On Hold",
      action: {
        icon: "pause-circle",
        effects: [],
        successMessage: "Role put on hold.",
      },
    },
    drop_hire: {
      to: "dropped",
      from: ["planned", "approved", "recruiting", "on_hold"],
      label: "Drop Role",
      action: {
        icon: "close-circle",
        style: "danger",
        confirm: {
          tone: "danger",
          title: "Drop this planned role?",
          confirmLabel: "Drop role",
        },
        effects: [],
        successMessage: "Role dropped from the plan.",
      },
    },
  },
} as const);
