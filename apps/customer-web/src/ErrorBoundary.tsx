import { Alert } from 'antd';
import { Component, type ReactNode } from 'react';

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  failed: boolean;
}

export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  public state: ErrorBoundaryState = { failed: false };

  public static getDerivedStateFromError(): ErrorBoundaryState {
    return { failed: true };
  }

  public componentDidCatch(): void {
    // The production telemetry adapter will record bounded error evidence later.
  }

  public render() {
    if (this.state.failed) {
      return (
        <main className="grid min-h-screen place-items-center bg-canvas p-6">
          <Alert
            className="max-w-xl"
            type="error"
            showIcon
            title="The toolchain shell could not render."
            description="No product operation was attempted. Reload after checking the build evidence."
          />
        </main>
      );
    }

    return this.props.children;
  }
}
