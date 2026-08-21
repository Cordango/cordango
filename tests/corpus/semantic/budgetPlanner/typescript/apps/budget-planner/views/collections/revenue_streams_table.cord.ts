import { defineCollectionView } from "@cord/sdk";

export default defineCollectionView({
  key: "revenue_streams_table",
  label: "Revenue Streams",
  entity: "revenue_stream",
  kind: "table",
  settings: {
    columns: [
      "name",
      "stream_type",
      "revenue_behaviour",
      "monthly_amount",
      "one_off_amount",
      "one_off_month",
      "one_off_date",
      "creator_share_pct",
      "net_monthly_amount",
      "start_month",
      "end_month",
      "customer",
    ],
    filterBar: {
      facets: ["scenario", "stream_type", "revenue_behaviour"],
      search: ["name", "customer"],
    },
    inlineEdit: true,
    allowDelete: true,
  },
} as const);
