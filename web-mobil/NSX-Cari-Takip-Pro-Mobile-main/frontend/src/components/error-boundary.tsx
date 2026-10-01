// App level error boundary, mounted once in app/_layout.tsx.
import { reloadAppAsync } from "expo";
import { Component, type ErrorInfo, type PropsWithChildren, useState } from "react";
import { Platform, Pressable, ScrollView, Text, View } from "react-native";
import Feather from "@react-native-vector-icons/feather";

import { makeStyles, fonts } from "@/src/theme";

type ErrorBoundaryState = { error: Error | null };

export class ErrorBoundary extends Component<PropsWithChildren, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("[ErrorBoundary] render crash:", error, info.componentStack ?? "");
  }

  resetError = (): void => {
    this.setState({ error: null });
  };

  render() {
    if (this.state.error) {
      return <ErrorFallback error={this.state.error} resetError={this.resetError} />;
    }
    return this.props.children;
  }
}

function ErrorFallback({ error, resetError }: { error: Error; resetError: () => void }) {
  const styles = useStyles();
  const [showDetails, setShowDetails] = useState(false);

  const handleReload = async () => {
    try {
      await reloadAppAsync();
    } catch {
      resetError();
    }
  };

  return (
    <View style={styles.container} testID="error-fallback">
      <View style={styles.content}>
        <View style={styles.iconWrap}>
          <Feather name="alert-triangle" size={28} color={styles._accent.color} />
        </View>
        <Text style={styles.title}>Bir şeyler ters gitti</Text>
        <Text style={styles.message}>
          Uygulamayı yenileyerek devam edebilirsiniz. Sorun sürerse oturumu kapatıp tekrar
          bağlanın.
        </Text>
        {__DEV__ ? <Text style={styles.devMessage}>{error.message}</Text> : null}
        <Pressable
          onPress={handleReload}
          testID="error-fallback-reload"
          accessibilityRole="button"
          style={({ pressed }) => [styles.button, pressed && styles.buttonPressed]}
        >
          <Feather name="refresh-cw" size={16} color={styles.buttonText.color} />
          <Text style={styles.buttonText}>Uygulamayı Yenile</Text>
        </Pressable>
        {__DEV__ ? (
          <Pressable
            onPress={() => setShowDetails((v) => !v)}
            accessibilityRole="button"
            hitSlop={8}
          >
            <Text style={styles.detailsToggle}>
              {showDetails ? "Detayları gizle" : "Detayları göster"}
            </Text>
          </Pressable>
        ) : null}
      </View>
      {__DEV__ && showDetails ? (
        <ScrollView style={styles.details} contentContainerStyle={styles.detailsContent}>
          <Text selectable style={styles.detailsText}>
            {error.stack ?? error.message}
          </Text>
        </ScrollView>
      ) : null}
    </View>
  );
}

const useStyles = makeStyles((colors) => ({
  container: {
    flex: 1,
    backgroundColor: colors.surface,
    justifyContent: "center",
    padding: 24,
  },
  content: {
    alignItems: "center",
    gap: 12,
  },
  iconWrap: {
    width: 64,
    height: 64,
    borderRadius: 22,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: colors.errorSoft,
    marginBottom: 4,
  },
  _accent: { color: colors.error },
  title: {
    color: colors.onSurface,
    fontFamily: fonts.displayMedium,
    fontSize: 22,
    letterSpacing: -0.4,
    textAlign: "center",
  },
  message: {
    color: colors.muted,
    fontFamily: fonts.body,
    fontSize: 15,
    lineHeight: 22,
    textAlign: "center",
    maxWidth: 320,
  },
  devMessage: {
    color: colors.error,
    fontFamily: fonts.body,
    fontSize: 13,
    textAlign: "center",
  },
  button: {
    marginTop: 8,
    backgroundColor: colors.brandPrimary,
    borderRadius: 999,
    paddingHorizontal: 24,
    paddingVertical: 14,
    minWidth: 200,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  buttonPressed: {
    opacity: 0.85,
  },
  buttonText: {
    color: colors.onBrandPrimary,
    fontFamily: fonts.bodyBold,
    fontSize: 15,
    textAlign: "center",
  },
  detailsToggle: {
    color: colors.muted,
    fontFamily: fonts.body,
    fontSize: 13,
    textDecorationLine: "underline",
    paddingVertical: 8,
  },
  details: {
    marginTop: 16,
    maxHeight: 260,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surfaceSecondary,
  },
  detailsContent: {
    padding: 12,
  },
  detailsText: {
    color: colors.onSurfaceSecondary,
    fontSize: 12,
    lineHeight: 18,
    fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }),
  },
}));
