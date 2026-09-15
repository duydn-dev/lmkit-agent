import 'package:flutter/material.dart';
import 'package:forui/forui.dart';

class AppTheme {
  AppTheme._();

  static const brandBlue = Color(0xFF123B72);
  static const brandBlueDark = Color(0xFF0A2A52);

  static ThemeData materialLight(FThemeData theme) =>
      theme.toApproximateMaterialTheme().copyWith(
        scaffoldBackgroundColor: const Color(0xFFF7F9FC),
        colorScheme: theme.toApproximateMaterialTheme().colorScheme.copyWith(
          primary: brandBlue,
          secondary: const Color(0xFF2563EB),
        ),
        inputDecorationTheme: const InputDecorationTheme(
          border: OutlineInputBorder(),
        ),
      );
}
