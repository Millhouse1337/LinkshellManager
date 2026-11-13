// @ts-check
const eslint = require("@eslint/js");
const { defineConfig } = require("eslint/config");
const tseslint = require("typescript-eslint");
const angular = require("angular-eslint");

module.exports = defineConfig([
  {
    files: ["**/*.ts"],
    extends: [
      eslint.configs.recommended,
      tseslint.configs.recommended,
      tseslint.configs.stylistic,
      angular.configs.tsRecommended,
    ],
    processor: angular.processInlineTemplates,
    rules: {
      "@angular-eslint/directive-selector": [
        "error",
        {
          type: "attribute",
          prefix: "app",
          style: "camelCase",
        },
      ],
      "@angular-eslint/component-selector": [
        "error",
        {
          type: "element",
          prefix: "app",
          style: "kebab-case",
        },
      ],
      // `any` usages are tracked as a backlog rather than blocking CI.
      "@typescript-eslint/no-explicit-any": "warn",
      // Dates and times are shown on the viewer's PROFILE clock (zoned-time.ts). These two write
      // the computer's zone instead, so one screen could show an event at two different times.
      "no-restricted-syntax": [
        "error",
        {
          selector: "CallExpression[callee.property.name=/^toLocale(Date|Time)String$/]",
          message: "Use activity.formatInViewerZone / formatDate (profile time zone) instead of toLocaleDateString/toLocaleTimeString.",
        },
      ],
    },
  },
  {
    files: ["**/*.html"],
    extends: [
      angular.configs.templateRecommended,
      angular.configs.templateAccessibility,
    ],
    rules: {
      // Allow the idiomatic `x != null` / `x == null` loose null checks; any
      // other loose (in)equality remains an error.
      "@angular-eslint/template/eqeqeq": ["error", { allowNullOrUndefined: true }],
      // Accessibility rules worth tracking, not worth blocking CI on yet.
      "@angular-eslint/template/label-has-associated-control": "warn",
      "@angular-eslint/template/click-events-have-key-events": "warn",
      "@angular-eslint/template/interactive-supports-focus": "warn",
      // Angular's date pipe writes the computer's time zone; the app shows the PROFILE zone.
      "no-restricted-syntax": [
        "error",
        {
          selector: "BindingPipe[name='date']",
          message: "Use the zonedTime pipe (profile time zone) instead of | date.",
        },
      ],
    },
  }
]);
