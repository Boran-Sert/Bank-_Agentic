package com.turkiyefinans.actions.atmbranch.assistant.aitext.adc;

public class AdcAIRequest {

    private String text;
    private String themeValue;
    private String sessionToken;

    public String getText() { return text; }
    public void setText(String text) { this.text = text; }

    public String getThemeValue() { return themeValue; }
    public void setThemeValue(String themeValue) { this.themeValue = themeValue; }

    public String getSessionToken() { return sessionToken; }
    public void setSessionToken(String sessionToken) { this.sessionToken = sessionToken; }
}
