import type { AnchorHTMLAttributes, ButtonHTMLAttributes, ReactNode } from "react";
import { cx, type LinkComponent } from "./util";

export type ButtonVariant = "primary" | "secondary" | "dark" | "outline" | "ghost" | "on-dark" | "danger";
export type ButtonSize = "sm" | "md" | "lg";

type Common = { variant?: ButtonVariant; size?: ButtonSize; full?: boolean; children: ReactNode };

/** Class names for a Violeta button, for elements the Button component does not render itself. */
export function buttonClassName({ variant = "primary", size = "md", full = false }: Omit<Common, "children"> = {}): string {
  return cx("vi-btn", `vi-btn--${variant}`, size !== "md" && `vi-btn--${size}`, full && "vi-btn--full");
}

export type ButtonProps = Common & ButtonHTMLAttributes<HTMLButtonElement> & { href?: undefined };
export type LinkButtonProps = Common & AnchorHTMLAttributes<HTMLAnchorElement> & { href: string; linkAs?: LinkComponent };

/** One primary per view; labels say the action. Renders a link when `href` is set, otherwise a button. */
export function Button(props: ButtonProps | LinkButtonProps) {
  if (props.href !== undefined) {
    const { variant, size, full, className, linkAs: Link = "a", ...rest } = props;
    return <Link {...rest} className={cx(buttonClassName({ variant, size, full }), className)} />;
  }
  const { variant, size, full, className, type = "button", ...rest } = props;
  return <button {...rest} type={type} className={cx(buttonClassName({ variant, size, full }), className)} />;
}
