import { createBrowserRouter } from "react-router-dom";

function HealthPage() {
  return (
    <main>
      <h1>Vinx ERP</h1>
      <p>Healthy</p>
    </main>
  );
}

export function createAppRouter() {
  return createBrowserRouter([
    {
      element: <HealthPage />,
      path: "/",
    },
  ]);
}
