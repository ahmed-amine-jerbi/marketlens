import { createBrowserRouter } from "react-router";

import { Layout, RouteError } from "./Layout";

/** Pages load on demand, so the first visit downloads only the shell and the page it opens. */
export const router = createBrowserRouter([
  {
    element: <Layout />,
    errorElement: <RouteError />,
    // Shown for the moment the first page's code loads.
    HydrateFallback: () => <div className="min-h-dvh bg-background" />,
    children: [
      {
        index: true,
        lazy: async () => ({ Component: (await import("../pages/HomePage")).HomePage }),
      },
      {
        path: "markets/:market",
        lazy: async () => ({ Component: (await import("../pages/MarketsPage")).MarketsPage }),
      },
      {
        path: "assets/:key",
        lazy: async () => ({ Component: (await import("../pages/AssetPage")).AssetPage }),
      },
      {
        path: "convert",
        lazy: async () => ({ Component: (await import("../pages/ConvertPage")).ConvertPage }),
      },
      {
        path: "data-sources",
        lazy: async () => ({
          Component: (await import("../pages/DataSourcesPage")).DataSourcesPage,
        }),
      },
      {
        path: "how-forecasts-work",
        lazy: async () => ({
          Component: (await import("../pages/ForecastMethodPage")).ForecastMethodPage,
        }),
      },
      {
        path: "*",
        lazy: async () => ({ Component: (await import("../pages/NotFoundPage")).NotFoundPage }),
      },
    ],
  },
]);
