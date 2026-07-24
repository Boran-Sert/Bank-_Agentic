package com.turkiyefinans.actions.atmbranch.assistant.aitext.adc;

public class AdcAIResponse {

    public enum Type { TEXT, OPEN_SCREEN, CONFIRM_PAYMENT }

    private Type type;

    // TEXT
    private String text;

    // OPEN_SCREEN
    private String mkCodeValue;
    private String mkCodesJson;

    // CONFIRM_PAYMENT
    private String cardNumber;
    private String cardProductName;
    private String amount;
    private String accountNo;

    public Type getType() { return type; }
    public void setType(Type type) { this.type = type; }

    public String getText() { return text; }
    public void setText(String text) { this.text = text; }

    public String getMkCodeValue() { return mkCodeValue; }
    public void setMkCodeValue(String mkCodeValue) { this.mkCodeValue = mkCodeValue; }

    public String getMkCodesJson() { return mkCodesJson; }
    public void setMkCodesJson(String mkCodesJson) { this.mkCodesJson = mkCodesJson; }

    public String getCardNumber() { return cardNumber; }
    public void setCardNumber(String cardNumber) { this.cardNumber = cardNumber; }

    public String getCardProductName() { return cardProductName; }
    public void setCardProductName(String cardProductName) { this.cardProductName = cardProductName; }

    public String getAmount() { return amount; }
    public void setAmount(String amount) { this.amount = amount; }

    public String getAccountNo() { return accountNo; }
    public void setAccountNo(String accountNo) { this.accountNo = accountNo; }
}
