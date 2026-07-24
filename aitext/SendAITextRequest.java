package com.turkiyefinans.actions.atmbranch.assistant.aitext;

import com.turkiyefinans.core.BaseRequest;

public class SendAITextRequest extends BaseRequest {

    private String text;
    private boolean prepare;

    public SendAITextRequest() {
    }

    public String getText() {
        return text;
    }

    public void setText(String text) {
        this.text = text;
    }

    public boolean isPrepare() {
        return prepare;
    }

    public void setPrepare(boolean prepare) {
        this.prepare = prepare;
    }
}
