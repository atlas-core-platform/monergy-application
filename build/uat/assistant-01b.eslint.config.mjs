import configuration from '../../eslint.config.mjs';
export default [...configuration, {
  files: ['build/uat/assistant-01b.*.ts'],
  languageOptions: { parserOptions: { projectService: false, project: './build/uat/assistant-01b.tsconfig.json' } },
}];
