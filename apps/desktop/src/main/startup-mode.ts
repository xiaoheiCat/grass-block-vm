/** 登录项传入 --autostart 时必须全程无窗口运行。 */
export function isAutostartLaunch(argv: readonly string[]): boolean {
  return argv.includes('--autostart');
}
